namespace DanceCommander.Core.Commands;

/// <summary>
/// How a typed query is matched against command titles, and in what order the matches come back.
/// </summary>
/// <remarks>
/// A bare word means "the title contains this", which is the same rule <c>NameQuery</c> states for
/// file names (§6.13). Learning to type in a pane is learning to type here, and a palette with its
/// own private matching dialect would be one more thing to hold in your head for no gain. Several
/// words are all required, in any order, so "copy path" finds "Copy path" whichever way round you
/// remember it.
/// </remarks>
public static class CommandSearch
{
    /// <summary>A command that does not match every term at all.</summary>
    public const int NoMatch = -1;

    /// <summary>
    /// How well a command answers a query: the sum over its terms of what kind of match each is,
    /// higher better, or <see cref="NoMatch"/> when some term is not found at all. The kinds are
    /// coarse on purpose (the whole title, a word of it, inside a word, a word of the group): the
    /// palette breaks ties between matches of the same kind by how often each command is chosen,
    /// and only a coarse rank leaves that room. "te" starts the word "Terminal" as truly as it
    /// starts "Text size", and the one used every day should come first. An empty query matches
    /// everything equally.
    /// </summary>
    public static int Score(CommandDefinition command, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return 0;

        var total = 0;
        foreach (var term in terms)
        {
            var best = ScoreTerm(command, term);
            if (best == NoMatch)
                return NoMatch;
            total += best;
        }

        return total;
    }

    /// <summary>
    /// True when the title starts with the query's first term. Weaker than how often a command is
    /// chosen, but with no history it still puts "Inspect archive" before "Deep inspect".
    /// </summary>
    public static bool LeadsWith(CommandDefinition command, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length > 0 && command.Title.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreTerm(CommandDefinition command, string term)
    {
        var title = command.Title;

        if (title.Equals(term, StringComparison.OrdinalIgnoreCase))
            return 4;

        // "folder" should find "New folder" ahead of anything that merely has the letters
        // somewhere inside a word, because a word is what the user typed.
        if (StartsAWord(title, term))
            return 3;
        if (title.Contains(term, StringComparison.OrdinalIgnoreCase))
            return 2;

        // The group is worth matching ("git" is how you look for all five git commands, and it
        // is not in any of their titles) but it is worth less than the title, so a command
        // actually called "Git status" leads the group it belongs to. Only from the start of a
        // word: "te" inside "Navigate" offered Back, Switch pane and every Go to.
        return command.Group is { } group && StartsAWord(group, term) ? 1 : NoMatch;
    }

    /// <summary>Whether any occurrence starts a word: "te" in "Delete the rest" does, at "the".</summary>
    private static bool StartsAWord(string text, string term)
    {
        for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (IsWordStart(text, at))
                return true;
        }

        return false;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]);
}

/// <summary>
/// The command palette's model: which commands are on offer, in what order (§13, "cheap once the
/// §1.2 seams exist").
/// </summary>
/// <remarks>
/// A renderer over the one registry, exactly as the F-key legend and the context menu are — it
/// registers nothing of its own and knows nothing a command did not already declare. The view
/// above it is a text box and a list; everything that decides what appears in that list is here,
/// where it can be tested without a window.
/// </remarks>
public sealed class CommandPalette
{
    /// <summary>
    /// How much history the top of the list is worth. Long enough to cover a working session's
    /// habits, short enough that a command used once last hour does not outrank the obvious answer.
    /// </summary>
    public const int RecentLimit = 12;

    private readonly List<string> _recent = [];
    private readonly CommandUsage _usage;

    /// <param name="usage">How often each command has been chosen, kept across sessions; when
    /// left out, this palette's own picks only.</param>
    public CommandPalette(CommandUsage? usage = null) => _usage = usage ?? new CommandUsage();

    /// <summary>
    /// Most recently invoked first. Session-lived: it describes what you are doing now, and it is
    /// what lifts commands to the top of the resting list, where a habit restored from last Tuesday
    /// would be a list in a surprising order. A typed query is different: you have said what you
    /// want, and which of the matches you usually mean is worth remembering for longer.
    /// </summary>
    public IReadOnlyList<string> Recent => _recent;

    /// <summary>
    /// What to show for a query, given the commands that apply where the user is standing.
    /// </summary>
    /// <param name="applicable">Usually <see cref="ICommandRegistry.ApplicableTo"/>.</param>
    public IReadOnlyList<CommandDefinition> Offer(IEnumerable<CommandDefinition> applicable, string query)
    {
        var scored = new List<(CommandDefinition Command, int Score)>();
        foreach (var command in applicable)
        {
            // A command with no group is a key rather than a menu item — Escape, and the palette
            // itself. The context menu draws the same line, and for the same reason: offering
            // "Dismiss" in a list of things you can do is offering an answer to nothing.
            if (command.Group is null)
                continue;

            var score = CommandSearch.Score(command, query);
            if (score != CommandSearch.NoMatch)
                scored.Add((command, score));
        }

        // Nothing typed yet, so nothing has been said about what matters: the list is there to be
        // read rather than ranked, and it reads in the groups the menus already use. Sorting the
        // resting list by how well it answers an empty question would shuffle it for no reason a
        // user could see.
        var ordered = query.Trim().Length == 0
            ? scored.OrderBy(entry => RecentRank(entry.Command.Id))
                .ThenBy(entry => entry.Command.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Command.Title, StringComparer.OrdinalIgnoreCase)
            : scored.OrderByDescending(entry => entry.Score)
                // Then what you usually choose, which is how "te" finds the terminal you open
                // every day ahead of a text size you set once.
                .ThenByDescending(entry => _usage.Count(entry.Command.Id))
                // With no history to go on, a title that starts with what you typed.
                .ThenByDescending(entry => CommandSearch.LeadsWith(entry.Command, query))
                // Then what you have been using. A palette is opened in the middle of doing
                // something, and what you did a minute ago is very often what you are about to do
                // again.
                .ThenBy(entry => RecentRank(entry.Command.Id))
                // Shortest title last: "Copy path" before "Copy this folder's path" for "copy".
                .ThenBy(entry => entry.Command.Title.Length)
                .ThenBy(entry => entry.Command.Title, StringComparer.OrdinalIgnoreCase);

        return ordered.Select(entry => entry.Command).ToList();
    }

    /// <summary>
    /// <see cref="Offer"/>, in sections by what each command acts on (§6.18), ranking inside each.
    /// At rest the sections read the selection, then this folder, then the window, and recently
    /// used rises within its own section, never above the rest: recency is a guess about which
    /// command, not about which subject. Once something is typed, the section holding the best
    /// answer comes first. Keeping the order fixed then put "Delete permanently" (a mid-word match
    /// for "te", in the selection's section) above "Open in Windows Terminal" (a word that starts
    /// with it, in the folder's), with the highlight and so Enter on the worse match, and on a
    /// command that deletes. Steady headers were not worth that. Empty sections are left out.
    /// </summary>
    public IReadOnlyList<PaletteSection> Sections(IEnumerable<CommandDefinition> applicable, string query)
    {
        // GroupBy keeps the order in which each subject first appears, so with a query each section
        // stands where its best command stands in the ranking.
        var sections = Offer(applicable, query).GroupBy(command => command.Subject);
        if (query.Trim().Length == 0)
            sections = sections.OrderBy(group => group.Key);

        return sections.Select(group => new PaletteSection(group.Key, group.ToList())).ToList();
    }

    public void Remember(CommandDefinition command)
    {
        _usage.Add(command.Id);
        _recent.Remove(command.Id);
        _recent.Insert(0, command.Id);
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
    }

    private int RecentRank(string id)
    {
        var index = _recent.IndexOf(id);
        return index < 0 ? int.MaxValue : index;
    }
}

/// <summary>One section of the palette: the commands that act on one subject, best match first.</summary>
public sealed record PaletteSection(CommandSubject Subject, IReadOnlyList<CommandDefinition> Commands);
