namespace DanceCommander.Extensibility;

/// <summary>Programs that want a terminal of their own, for the command line's hand-off (COMMAND_LINE_PLAN.md J).</summary>
public static class TerminalPrograms
{
    /// <summary>
    /// The programs that want a terminal of their own: run without one they hang, draw nothing,
    /// or quit at once. Python and node only as a bare REPL; with a script they are ordinary.
    /// </summary>
    private static readonly HashSet<string> Interactive = new(StringComparer.OrdinalIgnoreCase)
    {
        "vim", "vi", "nvim", "nano", "emacs", "less", "more", "man", "top", "htop", "btop", "ssh", "tmux", "screen",
    };

    private static readonly HashSet<string> Repls = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "node", "irb", "ghci", "psql", "mysql", "sqlite3",
    };

    /// <summary>
    /// The program a single-command line starts, if it is one that needs a terminal. Lines with an
    /// operator in them are left alone: `git log | less` is someone else's decision to make.
    /// </summary>
    public static string? NeedsTerminal(string line)
    {
        if (line.IndexOfAny(['|', ';', '&', '<', '>']) >= 0)
            return null;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return null;
        var program = Path.GetFileNameWithoutExtension(words[0]);
        if (Interactive.Contains(program))
            return program;
        return words.Length == 1 && Repls.Contains(program) ? program : null;
    }
}
