using System.Text;

namespace DanceCommander.Extensibility;

/// <summary>
/// One finished shell line as one argument on a process's command line, quoted for the program
/// that will read it back. For the command line's shells, whose lines are already expanded and
/// quoted for the shell: handed over through <see cref="ToolDefinition.VerbatimArgs"/>, so that
/// nothing expands placeholders in them a second time.
/// </summary>
/// <remarks>
/// Leaving it to .NET's argument list was wrong twice (review, 2026-10-12). It quotes only a value
/// with a space or a quote in it, and Git Bash's runtime splits, globs and strips single quotes from
/// one it didn't quote, so <c>'./$(x).exe'</c> ran x. And quoted, it follows the C runtime's rules,
/// where a backslash is literal unless a quote follows; Git Bash's runtime halves every pair, so
/// <c>\\server\share</c> arrived as <c>\server\share</c>.
/// </remarks>
public static class ProcessArguments
{
    /// <summary>For a program built on the MSYS/Cygwin runtime (Git Bash): every \ doubled, " as \".</summary>
    public static string ForMsys(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>For a program that reads its command line the C runtime's way (.NET, pwsh).</summary>
    public static string ForWindows(string value)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes count only before a quote: doubled there, and the quote escaped.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        // And before the closing quote, which is ours.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
